from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class ChangeSubscriptionResponse(AdditionalDataHolder, Parsable):
    """
    A change subscription; string? ChangeSubscriptionResponse.Secret is only returned when it is created (shown once).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The changeTypes property
    change_types: Optional[list[str]] = None
    # The clientState property
    client_state: Optional[str] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The expirationDateTime property
    expiration_date_time: Optional[datetime.datetime] = None
    # The id property
    id: Optional[UUID] = None
    # The notificationUrl property
    notification_url: Optional[str] = None
    # The resource property
    resource: Optional[str] = None
    # The secret property
    secret: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ChangeSubscriptionResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ChangeSubscriptionResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ChangeSubscriptionResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "changeTypes": lambda n : setattr(self, 'change_types', n.get_collection_of_primitive_values(str)),
            "clientState": lambda n : setattr(self, 'client_state', n.get_str_value()),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "expirationDateTime": lambda n : setattr(self, 'expiration_date_time', n.get_datetime_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "notificationUrl": lambda n : setattr(self, 'notification_url', n.get_str_value()),
            "resource": lambda n : setattr(self, 'resource', n.get_str_value()),
            "secret": lambda n : setattr(self, 'secret', n.get_str_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_collection_of_primitive_values("changeTypes", self.change_types)
        writer.write_str_value("clientState", self.client_state)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_datetime_value("expirationDateTime", self.expiration_date_time)
        writer.write_uuid_value("id", self.id)
        writer.write_str_value("notificationUrl", self.notification_url)
        writer.write_str_value("resource", self.resource)
        writer.write_str_value("secret", self.secret)
        writer.write_additional_data_value(self.additional_data)
    

