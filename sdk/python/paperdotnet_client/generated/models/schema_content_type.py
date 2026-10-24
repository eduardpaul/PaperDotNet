from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .schema_rule import SchemaRule

@dataclass
class SchemaContentType(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The is_default property
    is_default: Optional[bool] = False
    # The field_keys property
    field_keys: Optional[list[str]] = None
    # The id property
    id: Optional[str] = None
    # The key property
    key: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The rules property
    rules: Optional[list[SchemaRule]] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SchemaContentType:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SchemaContentType
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SchemaContentType()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .schema_rule import SchemaRule

        from .schema_rule import SchemaRule

        fields: dict[str, Callable[[Any], None]] = {
            "field_keys": lambda n : setattr(self, 'field_keys', n.get_collection_of_primitive_values(str)),
            "id": lambda n : setattr(self, 'id', n.get_str_value()),
            "is_default": lambda n : setattr(self, 'is_default', n.get_bool_value()),
            "key": lambda n : setattr(self, 'key', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "rules": lambda n : setattr(self, 'rules', n.get_collection_of_object_values(SchemaRule)),
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
        writer.write_collection_of_primitive_values("field_keys", self.field_keys)
        writer.write_str_value("id", self.id)
        writer.write_bool_value("is_default", self.is_default)
        writer.write_str_value("key", self.key)
        writer.write_str_value("name", self.name)
        writer.write_collection_of_object_values("rules", self.rules)
        writer.write_additional_data_value(self.additional_data)


