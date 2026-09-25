from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .term_label_dto import TermLabelDto

@dataclass
class UpdateTermRequest(AdditionalDataHolder, Parsable):
    """
    PATCH body: only the properties sent are changed. `parentId` moves the term (bool UpdateTermRequest.MoveToRoot moves it to the root).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The moveToRoot property
    move_to_root: Optional[bool] = False
    # The color property
    color: Optional[str] = None
    # The description property
    description: Optional[str] = None
    # The isDeprecated property
    is_deprecated: Optional[bool] = None
    # The labels property
    labels: Optional[list[TermLabelDto]] = None
    # The name property
    name: Optional[str] = None
    # The parentId property
    parent_id: Optional[UUID] = None
    # The sortOrder property
    sort_order: Optional[int] = None
    # The synonyms property
    synonyms: Optional[list[str]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> UpdateTermRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: UpdateTermRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return UpdateTermRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .term_label_dto import TermLabelDto

        from .term_label_dto import TermLabelDto

        fields: dict[str, Callable[[Any], None]] = {
            "color": lambda n : setattr(self, 'color', n.get_str_value()),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "isDeprecated": lambda n : setattr(self, 'is_deprecated', n.get_bool_value()),
            "labels": lambda n : setattr(self, 'labels', n.get_collection_of_object_values(TermLabelDto)),
            "moveToRoot": lambda n : setattr(self, 'move_to_root', n.get_bool_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "parentId": lambda n : setattr(self, 'parent_id', n.get_uuid_value()),
            "sortOrder": lambda n : setattr(self, 'sort_order', n.get_int_value()),
            "synonyms": lambda n : setattr(self, 'synonyms', n.get_collection_of_primitive_values(str)),
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
        writer.write_str_value("color", self.color)
        writer.write_str_value("description", self.description)
        writer.write_bool_value("isDeprecated", self.is_deprecated)
        writer.write_collection_of_object_values("labels", self.labels)
        writer.write_bool_value("moveToRoot", self.move_to_root)
        writer.write_str_value("name", self.name)
        writer.write_uuid_value("parentId", self.parent_id)
        writer.write_int_value("sortOrder", self.sort_order)
        writer.write_collection_of_primitive_values("synonyms", self.synonyms)
        writer.write_additional_data_value(self.additional_data)
    

